# 🖥️ Servidor web simple en C#

## Descripción general
Este proyecto implementa un **servidor web básico** desarrollado en **C#**, utilizando directamente **sockets tcp** y **sin emplear frameworks web externos**.

El servidor cumple con todos los requisitos planteados en el trabajo práctico, incluyendo la configuración externa, la concurrencia, la compresión de respuestas y el registro de solicitudes.

---
## Objetivos del trabajo
* Implementar un servidor web funcional basado en sockets tcp.
* Procesar solicitudes HTTP de tipo GET y POST.
* Servir archivos estáticos desde un directorio configurable.
* Manejar múltiples clientes concurrentemente.
* Registrar todas las solicitudes y errores.
* Aplicar compresión gzip para optimizar la transferencia de datos.
* Trabajar con archivos de configuración externos.

---
## Configuración (`config.json`)

```json
{  
    "Port": 8080,  
    "ContentRoot": "wwwroot"  
}
```

- **Port**: Puerto TCP en el que el servidor escucha conexiones entrantes.
- **ContentRoot**: Ruta a la carpeta desde la cual se servirán los archivos solicitados.

---
## Ejecución
1. Abrir una terminal en el directorio raíz del proyecto y ejecutar
```bash
dotnet run
```
2. Una vez iniciado el servidor, abrir cualquier navegador web e ingresar la siguiente url (el puerto puede variar si fue modificado en el archivo de configuración):
```arduino
http://localhost:8080/
```
3. Para detener la ejecución de forma segura, en la consola donde se esté ejecutando el proceso presionar:
```r
Ctrl + C
```
---
## Funcionamiento interno
### 1. Inicio del servidor
Se carga la configuración desde config.json y se inicia un TcpListener en el puerto especificado.
### 2. Aceptación de clientes
Por cada conexión entrante se crea una tarea asíncrona (ProcesarClienteAsync), lo que permite manejar múltiples solicitudes en paralelo.
### 3. Procesamiento de solicitud
Se leen los bytes de la red y se interpreta manualmente el formato del mensaje HTTP (línea de solicitud, headers, query params y body)
### 4. Detección del recurso solicitado
Se valida la ruta para evitar accesos fuera del directorio raíz y s edetermina el tipo de contenido según su extensión.
### 5. Envío de respuesta
- Si el archivo existe: se envía con sus headers y compresión GZIP opcional.
- Si no existe: se devuelve 404.html o un mensaje genérico.
### 6. Registro (logging)
Se guarda cada solicitud en un archivo log diario (logs/YYYY-MM-DD.log), incluyendo IP, método, URL, parámetros y body si corresponde.

Para evitar conflictos de escritura concurrente, se emplea un SemaphoreSlim que actúa como un mecanismo de exclusión mutua, garantizando que sólo una tarea escriba el archivo a la vez.
