FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env

RUN sed -i 's/\[openssl_init\]/# [openssl_init]/' /etc/ssl/openssl.cnf &&\
    printf "\n\n[openssl_init]\nssl_conf = ssl_sect" >> /etc/ssl/openssl.cnf &&\
    printf "\n\n[ssl_sect]\nsystem_default = ssl_default_sect" >> /etc/ssl/openssl.cnf &&\
    printf "\n\n[ssl_default_sect]\nMinProtocol = TLSv1\nCipherString = DEFAULT@SECLEVEL=0\n" >> /etc/ssl/openssl.cnf

RUN apt-get update && apt-get install -y --no-install-recommends curl

WORKDIR /app

COPY . ./

RUN dotnet restore Gnoss.BackgroundTask.Mail.OpenCORE/Gnoss.BackgroundTask.Mail/Gnoss.BackgroundTask.Mail.csproj

RUN dotnet publish Gnoss.BackgroundTask.Mail.OpenCORE/Gnoss.BackgroundTask.Mail/Gnoss.BackgroundTask.Mail.csproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:8.0

RUN sed -i 's/\[openssl_init\]/# [openssl_init]/' /etc/ssl/openssl.cnf &&\
    printf "\n\n[openssl_init]\nssl_conf = ssl_sect" >> /etc/ssl/openssl.cnf &&\
    printf "\n\n[ssl_sect]\nsystem_default = ssl_default_sect" >> /etc/ssl/openssl.cnf &&\
    printf "\n\n[ssl_default_sect]\nMinProtocol = TLSv1\nCipherString = DEFAULT@SECLEVEL=0\n" >> /etc/ssl/openssl.cnf

RUN apt-get update && apt-get install -y --no-install-recommends curl
RUN apt-get install -y --no-install-recommends gss-ntlmssp
RUN apt install -y mc sudo syslog-ng realmd gss-ntlmssp

WORKDIR /app
RUN groupadd -g 2000 gnoss && useradd -u 2000 -g 2000 gnoss &&\
    mkdir -p logs_correos logs trazas &&\
	chown gnoss:gnoss logs_correos logs trazas && chmod 777 logs_correos logs trazas
USER gnoss

COPY --from=build-env /app/out .

ENTRYPOINT ["dotnet", "Gnoss.BackgroundTask.Mail.dll"]
