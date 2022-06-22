![](https://content.gnoss.ws/imagenes/proyectos/personalizacion/7e72bf14-28b9-4beb-82f8-e32a3b49d9d3/cms/logognossazulprincipal.png)

# Gnoss.BackgroundTask.Mail.OpenCORE

Aplicación de segundo plano que se encarga de enviar todos los emails que se mandan a través de la plataforma. 

Este servicio está escuchando dos colas: 

* ColaNotificacion: La Web envía a esta cola los eventos de la plataforma que requieren del envío de un email: mensaje interno a otro usuario, invitación por email, olvidé mi contraseña...
* ColaCorreo: En esta cola se registran todos los emails enviados a través del API por aplicaciones externas. 

Configuración estandar de esta aplicación en el archivo docker-compose.yml: 

```yml
mail:
    image: gnoss/mail
    env_file: .env
    environment:
     virtuosoConnectionString: ${virtuosoConnectionString}
     acid: ${acid}
     base: ${base}
     RabbitMQ__colaServiciosWin: ${RabbitMQ}
     redis__redis__ip__master: ${redis__redis__ip__master}
     redis__redis__bd: ${redis__redis__bd}
     redis__redis__timeout: 60
     redis__recursos__ip__master: ${redis__recursos__ip__master}
     redis__recursos__bd: ${redis__recursos__bd}
     redis__recursos__timeout: 60
     idiomas: ${idiomas}
     Servicios__urlBase: ${Servicios__urlBase}
     connectionType: ${connectionType}
     intervalo: "100"
    volumes:
     - ./logs/mail:/app/logs
```

Se pueden consultar los posibles valores de configuración de cada parámetro aquí: https://github.com/equipognoss/Gnoss.Platform.Deploy
