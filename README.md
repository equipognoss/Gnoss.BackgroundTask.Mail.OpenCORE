# Gnoss.BackgroundTask.Mail.OpenCORE

Aplicación de segundo plano que se encarga de enviar todos los emails que se mandan a través de la plataforma. 

Configuración estandar de esta aplicación en el archivo docker-compose.yml: 

```yml
mail:
    image: mail
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
