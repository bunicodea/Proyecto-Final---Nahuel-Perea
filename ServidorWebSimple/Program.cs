using System;
using System.IO;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text.Json;

class ServidorWebSimple
{
    // --------------------------------------------------------------
    // Campos estáticos y constantes
    // --------------------------------------------------------------
    private static readonly SemaphoreSlim LogLock = new(1, 1);
    private const int BUFFER_SIZE = 8192;
    private const int MAX_HEADER_SIZE = 64 * 1024;
    private const int TIMEOUT_MS = 5000;
    private static readonly CancellationTokenSource MainCts = new();

    // --------------------------------------------------------------
    // Configuración del servidor
    // --------------------------------------------------------------
    class Config
    {
        public int Port { get; set; }
        public string ContentRoot { get; set; }
    }

    static Config CargarConfiguracion()
    {
        var json = File.ReadAllText("config.json"); // Lee el contenido completo
        return JsonSerializer.Deserialize<Config>(json)!; // Convierte el json a un objeto Config
    }

    // --------------------------------------------------------------
    // Encapsulación de solicitud HTTP
    // --------------------------------------------------------------
    class HttpRequest
    {
        public string Method { get; set; } = "";
        public string RawUrl { get; set; } = "";
        public string Path { get; set; } = "";
        public Dictionary<string, string> QueryParams { get; set; } = new();
        public Dictionary<string, string> Headers { get; set; } = new();
        public string Body { get; set; } = "";
    }

    // --------------------------------------------------------------
    // Tipos MIME soportados - Multipurpose Internet Mail Extensions
    // --------------------------------------------------------------
    static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        {".html", "text/html"},
        {".htm", "text/html"},
        {".css", "text/css"},
        {".js", "application/javascript"},
        {".json", "application/json"},
        {".png", "image/png"},
        {".jpg", "image/jpeg"},
        {".jpeg", "image/jpeg"},
        {".gif", "image/gif"},
        {".svg", "image/svg+xml"},
        {".ico", "image/x-icon"},
        {".txt", "text/plain"},
        {".wav", "audio/wav"},
        {".mp4", "video/mp4"},
        {".pdf", "application/pdf"}
    };

    // --------------------------------------------------------------
    // Punto de entrada principal
    // --------------------------------------------------------------
    static async Task Main(string[] args)
    {
        try
        {
            var config = CargarConfiguracion();
            var contentRoot = Path.GetFullPath(config.ContentRoot);
            Directory.CreateDirectory(contentRoot);
            Directory.CreateDirectory("logs");

            using var listener = new TcpListener(IPAddress.Any, config.Port);
            listener.Start();

            Console.WriteLine($"Servidor iniciado en puerto {config.Port}");
            Console.WriteLine($"Sirviendo archivos desde: {contentRoot}");
            Console.WriteLine("Presione Ctrl+C para detener el servidor");

            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true;
                MainCts.Cancel();
            };

            while (!MainCts.Token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(MainCts.Token);
                    _ = ProcesarClienteAsync(client, contentRoot); // no espera resultado, procesa en paralelo
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    await LogErrorAsync($"Error aceptando cliente: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fatal: {ex.Message}");
        }
        finally
        {
            Console.WriteLine("Servidor detenido.");
        }
    }

    // --------------------------------------------------------------
    // Procesamiento de cliente
    // --------------------------------------------------------------
    static async Task ProcesarClienteAsync(TcpClient client, string contentRoot)
    {
        using (client)
        {
            var remoteEp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

            try
            {
                using var stream = client.GetStream();
                var request = await LeerSolicitudHttpAsync(stream);
                if (request == null) return;

                await LogRequestAsync(request, remoteEp);

                if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    !request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    await EnviarRespuestaSimpleAsync(stream, "501 Not Implemented", 
                        "text/plain", "Método no soportado");
                    return;
                }

                var path = request.Path == "/" ? "/index.html" : request.Path;

                var fullPath = ObtenerRutaSegura(path, contentRoot); // ruta completa al archivo pedido
                
                if (fullPath == null)
                {
                    await EnviarRespuestaSimpleAsync(stream, "403 Forbidden", 
                        "text/plain", "Acceso denegado");
                    return;
                }

                if (!File.Exists(fullPath))
                {
                    await Enviar404Async(stream, contentRoot);
                    return;
                }

                await EnviarArchivoAsync(stream, fullPath, request.Headers);
            }
            catch (Exception ex)
            {
                await LogErrorAsync($"Error procesando cliente {remoteEp}: {ex.Message}");
            }
        }
    }

    // --------------------------------------------------------------
    // Lectura y parseo de solicitud HTTP
    // --------------------------------------------------------------
    static async Task<HttpRequest?> LeerSolicitudHttpAsync(NetworkStream stream) // permite leer los bytes enviados por el cliente
    {
        var headerBuilder = new StringBuilder(); // acumula
        var buffer = new byte[BUFFER_SIZE];
        var totalRead = 0;
        var request = new HttpRequest();

    // Leer headers
    while (totalRead < MAX_HEADER_SIZE)
    {
        using var headerCts = new CancellationTokenSource(TIMEOUT_MS);
        var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, headerCts.Token);
        if (bytesRead == 0) return null;

        totalRead += bytesRead;
        var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead); // convierte bytes a string
        headerBuilder.Append(chunk); // acumula y guarda los headers en un string builder

        var headerStr = headerBuilder.ToString();
        var headerEnd = headerStr.IndexOf("\r\n\r\n");
        
        if (headerEnd >= 0)
        {
            var headerText = headerStr.Substring(0, headerEnd); // header sin body
            var remainingData = headerStr.Substring(headerEnd + 4); // datos después de los headers, rnrnrn = 4 chars

            // Parsear la línea de solicitud
            var headerLines = headerText.Split("\r\n");
            var requestLine = headerLines[0].Split(' '); // método, ruta, versión http - primera línea
            
            if (requestLine.Length < 2) return null;

            request.Method = requestLine[0];
            request.RawUrl = requestLine[1];

            // Parsear URL y query params
            var urlParts = request.RawUrl.Split('?', 2);
            request.Path = Uri.UnescapeDataString(urlParts[0]); // decodificar ruta - 1 = parámetros, si hay
            if (urlParts.Length > 1)
            {
                request.QueryParams = ParseQueryString(urlParts[1]);
            }

            // Parsear headers
            for (int i = 1; i < headerLines.Length; i++)
            {
                var line = headerLines[i];
                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    var name = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1).Trim();
                    request.Headers[name] = value;
                }
            }

            // Leer body si es POST
            if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                request.Headers.TryGetValue("Content-Length", out var lenStr) &&    // content -> lenstr -> int contentlength
                int.TryParse(lenStr, out var contentLength))
            {
                var bodyBuilder = new MemoryStream(); // espacio de memoria para almacenar el body
                
                // Procesar datos ya leídos
                if (!string.IsNullOrEmpty(remainingData))
                {
                    var remainingBytes = Encoding.UTF8.GetBytes(remainingData);
                    bodyBuilder.Write(remainingBytes, 0, remainingBytes.Length);
                }

                // Leer el resto del body
                var remaining = contentLength - (int)bodyBuilder.Length;
                
                while (remaining > 0)
                {
                    using var bodyCts = new CancellationTokenSource(TIMEOUT_MS);
                    var toRead = Math.Min(buffer.Length, remaining); // cantidad exacta que se van a poder leer
                    var read = await stream.ReadAsync(buffer, 0, toRead, bodyCts.Token); // desde 0 hasta toRead
                    
                    if (read == 0) break; // cliente cerró conexión antes de enviar todo el body

                    bodyBuilder.Write(buffer, 0, read);
                    remaining -= read;
                }

                request.Body = Encoding.UTF8.GetString(bodyBuilder.ToArray());
            }

            return request; // devuelve el objeto HttpRequest con método, url, encabezados y body
        }
    }

    return null;
}

    // --------------------------------------------------------------
    // Envío de archivos
    // --------------------------------------------------------------
    static async Task EnviarArchivoAsync(NetworkStream stream, string path, 
        Dictionary<string, string> requestHeaders)
    {
        var mime = ObtenerMimeType(path); // esencial para content-type
        var fileInfo = new FileInfo(path); // tamaño, nombre y fecha de modificación
        
        var acceptGzip = requestHeaders.TryGetValue("Accept-Encoding", out var enc) && 
                        enc.Contains("gzip", StringComparison.OrdinalIgnoreCase);
        var shouldCompress = acceptGzip && EsComprimible(mime);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = mime
        };

        if (shouldCompress)
        {
            headers["Content-Encoding"] = "gzip";
            headers["Vary"] = "Accept-Encoding"; // si dos clientes piden el mismo recurso, uno con gzip y otro sin gzip, el servidor debe responder de forma diferente
            
            await EnviarHeadersAsync(stream, "200 OK", headers); // construye la linea de estado http seguida de todos los encabezados

            using var gzip = new GZipStream(stream, CompressionLevel.Fastest, true);

            await fs.CopyToAsync(gzip); // envío del archivo comprimido
        }
        else
        {
            headers["Content-Length"] = fileInfo.Length.ToString();
            await EnviarHeadersAsync(stream, "200 OK", headers);
            await fs.CopyToAsync(stream);
        }
    }

    // --------------------------------------------------------------
    // Manejo de respuestas HTTP
    // --------------------------------------------------------------
    static async Task Enviar404Async(NetworkStream stream, string contentRoot)
    {
        var custom404 = Path.Combine(contentRoot, "404.html");
        string content;
        string contentType = "text/html";

        if (File.Exists(custom404))
        {
            content = await File.ReadAllTextAsync(custom404);
        }
        else
        {
            content = "<h1>404 - No encontrado</h1>";
        }

        await EnviarRespuestaSimpleAsync(stream, "404 Not Found", contentType, content);
    }

    static async Task EnviarRespuestaSimpleAsync(NetworkStream stream, string status, 
        string contentType, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = contentType,
            ["Content-Length"] = bytes.Length.ToString()
        };

        await EnviarHeadersAsync(stream, status, headers);
        await stream.WriteAsync(bytes); // envía el cuerpo del mensaje en bytes
    }

    static async Task EnviarHeadersAsync(NetworkStream stream, string status, 
        Dictionary<string, string> headers)
    {
        var sb = new StringBuilder(); // construye la línea de estado http seguida de todos los encabezados
        sb.AppendLine($"HTTP/1.1 {status}");
        foreach (var header in headers)
        {
            sb.AppendLine($"{header.Key}: {header.Value}");
        }
        sb.AppendLine(); // ya no hay más encabezados

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes);
    }

    // --------------------------------------------------------------
    // Logging thread-safe
    // --------------------------------------------------------------
    static async Task LogRequestAsync(HttpRequest request, string clientIp)
    {
        var sb = new StringBuilder();

        sb.AppendLine("-------------------------");
        sb.AppendLine($"Fecha y hora: {DateTime.UtcNow:O}");
        sb.AppendLine($"IP del cliente: {clientIp}");
        
        if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)) // solo loguea los datos recibidos (body)
        {
            sb.AppendLine("Datos recibidos:");
            sb.AppendLine(request.Body);
        }
        else if (request.QueryParams.Count > 0) // loguea los parámetros de consulta (query string)
        {
            sb.AppendLine("Parámetros de consulta:");
            foreach (var param in request.QueryParams)
            {
                sb.AppendLine($"  {param.Key} = {param.Value}");
            }
        }
        else // otra solicitud, sólo método y url
        {
            sb.AppendLine($"Método: {request.Method}");
            sb.AppendLine($"URL: {request.RawUrl}");
        }

        sb.AppendLine();

        await LogLock.WaitAsync(); // la tarea quiere entrar pero espera su turno

        try
        {
            var logPath = Path.Combine("logs", $"{DateTime.UtcNow:yyyy-MM-dd}.log");
            await File.AppendAllTextAsync(logPath, sb.ToString());
        }
        finally
        {
            LogLock.Release();
        }
    }

    static async Task LogErrorAsync(string mensaje)
    {
        await LogLock.WaitAsync();
        try
        {
            var logPath = Path.Combine("logs", $"{DateTime.UtcNow:yyyy-MM-dd}.log");
            await File.AppendAllTextAsync(logPath, 
                $"ERROR: {mensaje} ({DateTime.UtcNow:O}){Environment.NewLine}");
        }
        finally
        {
            LogLock.Release();
        }
    }

    // --------------------------------------------------------------
    // Métodos auxiliares
    // --------------------------------------------------------------
    static string? ObtenerRutaSegura(string requestPath, string contentRoot) // previene ataques de path traversal
    {
        var decoded = WebUtility.UrlDecode(requestPath.TrimStart('/'));
        var fullPath = Path.GetFullPath(Path.Combine(contentRoot, decoded)); // combina la ruta del content root con la ruta solicitada
        var contentRootNormalized = contentRoot.EndsWith(Path.DirectorySeparatorChar.ToString()) 
            ? contentRoot 
            : contentRoot + Path.DirectorySeparatorChar;
        
        return fullPath.StartsWith(contentRootNormalized, StringComparison.OrdinalIgnoreCase) 
            ? fullPath 
            : null;
    }

    static string ObtenerMimeType(string path)
    {
        var ext = Path.GetExtension(path);
        return MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";
    }

    static bool EsComprimible(string mime) =>
        mime.StartsWith("text/") || 
        mime == "application/javascript" || 
        mime == "application/json" || 
        mime.EndsWith("xml");

    static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(query)) return dict;

        var parts = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2);
            var key = WebUtility.UrlDecode(keyValue[0]); // quita caracteres especiales, url decode decodifica
            var value = keyValue.Length > 1 ? WebUtility.UrlDecode(keyValue[1]) : "";
            dict[key] = value;
        }

        return dict;
    }
}