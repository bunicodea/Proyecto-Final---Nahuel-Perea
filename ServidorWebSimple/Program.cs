using System;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text.Json;

class ServidorWebSimple
{
    // --------------------------------------------------------------
    // Clase interna de configuración del servidor
    // Carga los valores de "config.json" 
    // --------------------------------------------------------------
    class Config
    {
        public int Port { get; set; } 
        public string ContentRoot { get; set; }
    }

    static Config CargarConfig()
    {
        var json = File.ReadAllText("config.json"); // Lee el contenido completo
        return JsonSerializer.Deserialize<Config>(json)!; // Convierte el json a un objeto Config
    }

    // --------------------------------------------------------------
    // Tabla de tipos MIME para enviar el Content-Type correcto
    // Sirve para servir los archivos correctamente y saber qué puede comprimir y qué no
    // --------------------------------------------------------------

    static readonly Dictionary<string, string> MimeTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        {".html","text/html"},
        {".htm","text/html"},
        {".css","text/css"},
        {".js","application/javascript"},
        {".json","application/json"},
        {".png","image/png"},
        {".jpg","image/jpeg"},
        {".jpeg","image/jpeg"},
        {".gif","image/gif"},
        {".svg","image/svg+xml"},
        {".ico","image/x-icon"},
        {".txt","text/plain"},
        {".wav","audio/wav"},
        {".mp4","video/mp4"},
        {".pdf","application/pdf"},
    };

    static string GetMime(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext != null && MimeTypes.TryGetValue(ext, out var m)) return m;
        return "application/octet-stream"; // Tipo genérico
    }

    // --------------------------------------------------------------
    // Punto de entrada principal (asincrónico)
    // Acepta conexiones TCP sin bloquear el hilo principal
    // Cada conexión se maneja de forma independiente en un Task separado para permitir concurrencia
    // --------------------------------------------------------------
    static async Task Main(string[] args)
    {
        var cfg = CargarConfig();
        var contentRoot = Path.GetFullPath(cfg.ContentRoot); // Obtiene la ruta absoluta del directorio raíz
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory("logs");

        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); // Crea un socket TCP que escuchará conexiones entrantes

        listener.Bind(new IPEndPoint(IPAddress.Any, cfg.Port)); // Acepta conexiones desde cualquier IP en el puerto configurado
        listener.Listen(100); // Permite hasta 100 conexiones simultáneas en cola

        // Con lo anterior el puerto es configurable y se asegura la concurrencia

        Console.WriteLine($"Servidor iniciado en puerto {cfg.Port}. Sirviendo desde: {contentRoot}");
        Console.WriteLine("Ctrl + C para detener.");

        var stop = false;
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop = true; listener.Close(); };

        while (!stop) // Bucle principal de escucha: acepta clientes y los atiende en tareas separadas
        {
            Socket client = null;
            try
            {
                client = await listener.AcceptAsync(); // Espera una conexión entrante
                _ = Task.Run(() => HandleClient(client, contentRoot)); // Atiende al cliente en una tarea independiente
            }
            catch (ObjectDisposedException) { break; } // Sale del bucle si se cierra el listener
            catch (Exception ex)
            {
                Console.WriteLine("Error accept: " + ex.Message);
            }
        }

        Console.WriteLine("Servidor detenido.");
    }

    // --------------------------------------------------------------
    // Método asincrónico que maneja cada conexión individual de cliente
    // Socket client es el socket específico para esa conexión
    // contentRoot es la carpeta desde donde se sirven los archivos
    // Lee la solicitud HTTP, procesa y envía la respuesta
    // --------------------------------------------------------------
    static async Task HandleClient(Socket client, string contentRoot)
    {
        var remoteEP = client.RemoteEndPoint?.ToString() ?? "unknown"; // IP y puerto del cliente conectado. Si no está disponible, entonces: unknown
        // Con esto obtenemos la IP de origen para los logs

        try
        {
            using var network = new NetworkStream(client, ownsSocket: true);
            // NetworkStream envuelve el socket para facilitar la lectura y escritura de datos. 
            // ownsSocket: true indica que al cerrar el stream se cerrará también el socket subyacente.
            network.ReadTimeout = 5000;
            network.WriteTimeout = 5000;

            // ------------------ Lectura del encabezado HTTP ------------------
            // Lee datos del NetworkStream hasta encontrar el final de los headers (\r\n\r\n)
            // Con esto el servidor parsea HTTP manualmente sin librerías externas
            var headerBuilder = new StringBuilder();
            var buffer = new byte[8192];
            int bytesRead = 0;
            int totalRead = 0;
            bool headersComplete = false;
            while (!headersComplete && (bytesRead = await network.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += bytesRead;
                var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                headerBuilder.Append(chunk);
                if (headerBuilder.ToString().Contains("\r\n\r\n"))
                {
                    headersComplete = true;
                    break;
                }
                // Protección frente a ataques de header gigante
                if (totalRead > 64 * 1024) break; // Protección contra headers excesivos
            }

            // Esto separa headers y body (si hay body).
            // headerText contiene sólo los headers
            // remainingAfterHeaders contiene cualquier dato leído después de los headers (parte del body)
            var headerStr = headerBuilder.ToString();
            if (string.IsNullOrEmpty(headerStr))
                return;

            var headerParts = headerStr.Split(new string[] { "\r\n\r\n" }, 2, StringSplitOptions.None);
            var headerText = headerParts[0];
            var remainingAfterHeaders = headerParts.Length > 1 ? headerParts[1] : "";

            // ------------------ Parseo de la línea de solicitud ------------------
            // Separa método (GET o POST), rawUrl (index.html) y httpVersion (HTTP/1.1)
            using var reader = new StringReader(headerText);
            var requestLine = reader.ReadLine();
            if (requestLine == null) return;
            var requestTokens = requestLine.Split(' ');
            if (requestTokens.Length < 2) return;
            var method = requestTokens[0];
            var rawUrl = requestTokens[1];
            var httpVersion = requestTokens.Length > 2 ? requestTokens[2] : "HTTP/1.1";

            // ------------------ Parseo de headers ------------------
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    var name = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1).Trim();
                    headers[name] = value;
                }
            }

            // ------------------ Parseo de la URL y parámetros de consulta ------------------
            // ParseQueryString es un método auxiliar para convertirlo en un diccionario
            // Cumple el requisito de manejar parámetros de consulta desde la URL (sólo loguearlos)
            string pathPart = rawUrl;
            string queryString = "";
            var qIdx = rawUrl.IndexOf('?');
            if (qIdx >= 0)
            {
                pathPart = rawUrl.Substring(0, qIdx);
                queryString = rawUrl.Substring(qIdx + 1);
            }
            var queryParams = ParseQueryString(queryString);

            // ------------------ Lectura del cuerpo (para POST) ------------------
            // Si el método es POST, busca el header Content-Length para saber cuánto leer del body
            // Cumple el requisito de aceptar solicitudes POST y sólo loguear los datos recibidos
            string body = "";
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                int contentLength = 0;
                if (headers.TryGetValue("Content-Length", out var clStr))
                    int.TryParse(clStr, out contentLength);

                var bodyBuilder = new MemoryStream();
                // El bloque remainingAfterHeaders puede contener parte del body
                if (!string.IsNullOrEmpty(remainingAfterHeaders))
                {
                    var b = Encoding.UTF8.GetBytes(remainingAfterHeaders);
                    bodyBuilder.Write(b, 0, b.Length);
                }

                int remaining = contentLength - (int)bodyBuilder.Length;
                while (remaining > 0)
                {
                    int toRead = Math.Min(buffer.Length, remaining);
                    int n = await network.ReadAsync(buffer, 0, toRead);
                    if (n <= 0) break;
                    bodyBuilder.Write(buffer, 0, n);
                    remaining -= n;
                }

                body = Encoding.UTF8.GetString(bodyBuilder.ToArray());
            }

            // ------------------ Logueo de la solicitud ------------------
            LogRequest(method, rawUrl, headers, queryParams, body, client);

            // ------------------ Validación de métodos soportados ------------------
            // Si el método es distinto a GET o POST, responde con 501 Not Implemented
            if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                !method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSimpleResponse(network, "501 Not Implemented", "text/plain", Encoding.UTF8.GetBytes("501 Not Implemented"), null, httpVersion);
                return;
            }

            // ------------------ Resolución de rutas ------------------
            // Normalizo el path para evitar problemas con encoding de URL (ej: %20 = espacio)
            var urlPath = Uri.UnescapeDataString(pathPart);
            if (urlPath == "/") urlPath = "/index.html"; // Si no se especifica archivo, sirve index.html por defecto

            // Evito ataques de directory traversal:
            var requestedPath = urlPath.TrimStart('/');
            var fullPath = Path.GetFullPath(Path.Combine(contentRoot, requestedPath));
            if (!fullPath.StartsWith(contentRoot))
            {
                // Si se intenta acceder fuera de contentRoot, responde con 403 Forbidden
                await WriteSimpleResponse(network, "403 Forbidden", "text/plain", Encoding.UTF8.GetBytes("403 Forbidden"), null, httpVersion);
                return;
            }

            // ------------------ Envío del archivo solicitado ------------------
            if (File.Exists(fullPath))
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(fullPath); // Leo el archivo en bytes
                var mime = GetMime(fullPath); // Obtengo el tipo MIME según la extensión

                // Verifico si el cliente acepta gzip y el recurso es compresible (text, js, css, html, json)
                var acceptEnc = headers.ContainsKey("Accept-Encoding") ? headers["Accept-Encoding"] : "";
                bool gzipOk = acceptEnc.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                              && (mime.StartsWith("text/") || mime == "application/javascript" || mime == "application/json" || mime.EndsWith("xml"));

                if (gzipOk) // Si el cliente acepta gzip, comprimo y envío con Content-Encoding: gzip
                {
                    byte[] gzipped;
                    using (var ms = new MemoryStream())
                    {
                        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) // GZipStream para comprimir
                        {
                            await gz.WriteAsync(fileBytes, 0, fileBytes.Length);
                        }
                        gzipped = ms.ToArray();
                    }

                    // Envío la respuesta con los headers adecuados
                    var headersResp = new Dictionary<string, string>
                    {
                        {"Content-Encoding", "gzip"},
                        {"Content-Type", mime},
                        {"Vary", "Accept-Encoding"}
                    };
                    await WriteSimpleResponse(network, "200 OK", mime, gzipped, headersResp, httpVersion);

                    // Cumple el requisito de utilizar compresión de archivos para responder
                }
                else
                {
                    var headersResp = new Dictionary<string, string>
                    {
                        {"Content-Type", mime}
                    };
                    await WriteSimpleResponse(network, "200 OK", mime, fileBytes, headersResp, httpVersion);

                    // Envía el archivo sin comprimir
                }
            }
            else
            {
                // ------------------ Respuesta 404 personalizada ------------------
                var custom404 = Path.Combine(contentRoot, "404.html");

                // Opción de seguridad: verificar que exista el archivo
                if (!File.Exists(custom404))
                    throw new FileNotFoundException("No se encontró el archivo 404.html en la carpeta raíz.");

                // Carga el contenido del 404.html y lo devuelve con código 404.
                var bytes = await File.ReadAllBytesAsync(custom404);
                var headersResp = new Dictionary<string, string> { { "Content-Type", "text/html" } };
                await WriteStatusResponse(network, "404 Not Found", bytes, headersResp, httpVersion);
            }
        }
        catch (Exception ex)
        {
            // Cualquier error durante el manejo del cliente se registra en consola
            Console.WriteLine($"Error manejando cliente {remoteEP}: {ex.Message}");
        }
        finally
        {
            // Se cierra el socket y la conexión de manera limpia y ordenada
            try { client.Shutdown(SocketShutdown.Both); } catch { }
            client.Close();
        }
    }

    // --------------------------------------------------------------
    // Esta función es la encargada de escribir la respuesta HTTP completa en el NetworkStream
    // Cada vez que el servidor debe devolver algo, usa esta función
    // Construye y envía una respuesta HTTP completa
    // --------------------------------------------------------------
    static async Task WriteSimpleResponse(NetworkStream network, string status, string contentType, byte[] body, Dictionary<string, string>? extraHeaders, string httpVersion)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        headers["Content-Length"] = body?.Length.ToString() ?? "0"; // Crea un diccionario de headers HTTP, calcula el Content-Length para que el navegador sepa cuánto leer y, si no hay cuerpo, el tamaño es 0
        // Con esto construyo las respuestas HTTP de forma manual

        if (!headers.ContainsKey("Content-Type") && !string.IsNullOrEmpty(contentType))
            headers["Content-Type"] = contentType; // Asegura que la respuesta tenga un tipo de contenido. Si Content-Type no se añadó antes, usa el que se pasó por parámetro

        if (extraHeaders != null)
        {
            foreach (var kv in extraHeaders) headers[kv.Key] = kv.Value;
            // Permite incluir headers adicionales (Content-Encoding: gzip, por ejemplo)
            // Se usa para respuestas comprimidas o con metadatos especiales
        }

        var sb = new StringBuilder();
        sb.Append($"{httpVersion} {status}\r\n"); // Construye la primera línea de la respuesta HTTP con la versión y el estado

        foreach (var kv in headers)
        {
            sb.Append($"{kv.Key}: {kv.Value}\r\n"); // Agrega todos los headers al texto de respuesta
        }
        sb.Append("\r\n"); // Línea en blanco que separa headers del body

        var headBytes = Encoding.UTF8.GetBytes(sb.ToString()); // Convierte headers a bytes UTF-8 para enviar por la red
        await network.WriteAsync(headBytes, 0, headBytes.Length); // Envía los headers al cliente a través de NetworkStream

        if (body != null && body.Length > 0) // Si hay cuerpo, lo envía después de los headers
            await network.WriteAsync(body, 0, body.Length);
    }

    // Esto es un atajo para evitar repetición en el código.
    // Variante de WriteSimpleResponse para respuestas con estado específico (404, 403, etc.)
    static async Task WriteStatusResponse(NetworkStream network, string status, byte[] body, Dictionary<string, string>? extraHeaders, string httpVersion)
    {
        await WriteSimpleResponse(
            network,
            status,
            extraHeaders != null && extraHeaders.ContainsKey("Content-Type") ? extraHeaders["Content-Type"] : "text/html",
            body,
            extraHeaders,
            httpVersion
        );
    }

    // --------------------------------------------------------------
    // Convierte una query string a diccionario clave-valor
    // --------------------------------------------------------------
    static Dictionary<string, string> ParseQueryString(string q)
    {
        var dict = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(q)) return dict; // Si la query string está vacía, devuelve un diccionario vacío

        var parts = q.Split('&', StringSplitOptions.RemoveEmptyEntries); // Divide la query por & para separar los pares
        foreach (var p in parts)
        {
            var idx = p.IndexOf('='); // Divide cada par por = para separar clave y valor
            if (idx >= 0)
            {
                // UrlDecode para traducir caracteres especiales (ej: %20 = espacio)
                var k = WebUtility.UrlDecode(p.Substring(0, idx));
                var v = WebUtility.UrlDecode(p.Substring(idx + 1));
                dict[k] = v;
            }
            else
            {
                dict[WebUtility.UrlDecode(p)] = "";
            }
        }
        return dict;
    }

    // --------------------------------------------------------------
    // LogRequest tiene dos sobrecargas. Una recibe el socket del cliente para extraer la IP y la otra recibe la IP directamente (para el log)
    // --------------------------------------------------------------
    static void LogRequest(string method, string rawUrl, Dictionary<string, string> headers, Dictionary<string, string> queryParams, string body, Socket client)
    {
        var ip = (client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown"; // Obtiene la IP del cliente desde el socket
        LogRequest(method, rawUrl, headers, queryParams, body, ip); // Llama a la otra sobrecarga pasando la IP
    }

    static void LogRequest(string method, string rawUrl, Dictionary<string,string> headers, Dictionary<string,string> queryParams, string body, string ip)
    {
        var date = DateTime.UtcNow;
        var logfile = Path.Combine("logs", date.ToString("yyyy-MM-dd") + ".log"); // Genero el nombre del archivo de log según la fecha actual
        var sb = new StringBuilder();
        sb.AppendLine("-------------------------");
        sb.AppendLine($"Fecha y hora: {date:O}");
        sb.AppendLine($"IP del cliente: {ip}");
        sb.AppendLine($"Método: {method}");
        sb.AppendLine($"URL: {rawUrl}");
        if (queryParams.Count > 0)
        {
            sb.AppendLine("Parámetros de query:"); // Si los hay, los listo
            foreach (var q in queryParams)
                sb.AppendLine($"  {q.Key} = {q.Value}");
        }
        sb.AppendLine("Headers:");
        foreach (var h in headers)
            sb.AppendLine($"  {h.Key}: {h.Value}"); // Agrego todos los headers HTTP que el cliente envió (Host, User-Agent, etc)

        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase)) // Si la solicitud es POST, guardo el contenido del cuerpo
        {
            sb.AppendLine("Body:");
            sb.AppendLine(body);
        }
        sb.AppendLine();

        // Escribo el log en el archivo correspondiente
        try
        {
            File.AppendAllText(logfile, sb.ToString()); // AppendAllText añade al final del archivo sin sobrescribir lo que ya había
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error escribiendo log: " + ex.Message);
        }
    }
}