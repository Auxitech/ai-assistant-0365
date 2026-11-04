FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MailRouter.Web/MailRouter.Web.csproj src/MailRouter.Web/
RUN dotnet restore src/MailRouter.Web/MailRouter.Web.csproj
COPY src/ src/
RUN dotnet publish src/MailRouter.Web/MailRouter.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The aspnet base image already listens on 8080 (HTTP_PORTS).
ENV DataDirectory=/data
RUN mkdir -p /data && chown -R app:app /data
USER app
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "MailRouter.Web.dll"]
