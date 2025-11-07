# 🖥️ Servidor web simple en C#

## Descripción general
Este proyecto implementa un **servidor web básico** desarrollado en **C#**, utilizando directamente **sockets tcp** y **sin emplear frameworks web externos**.

El servidor cumple con todos los requisitos planteados en el trabajo práctico, incluyendo la configuración externa, la concurrencia, la compresión de respuestas y el registro de solicitudes.

## Objetivos del trabajo
* Implementar un servidor web funcional basado en sockets tcp.
* Procesar solicitudes HTTP de tipo GET y POST.
* Servir archivos estáticos desde un directorio configurable.
* Manejar múltiples clientes concurrentemente.
* Registrar todas las solicitudes y errores.
* Aplicar compresión gzip para optimizar la transferencia de datos.
* Trabajar con archivos de configuración externos.

## Configuración (`config.json`)

{  
    "Port": 8080,  
    "ContentRoot": "wwwroot"  
}

- **Port**: Puerto TCP en el que el servidor escucha conexiones entrantes.
- **ContentRoot**: Ruta a la carpeta desde la cual se servirán los archivos solicitados.
