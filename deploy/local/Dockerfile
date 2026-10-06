FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
ARG SERVICE
WORKDIR /source
COPY . .
RUN dotnet publish "src/${SERVICE}/Vwp.${SERVICE}.Api/Vwp.${SERVICE}.Api.csproj" -c Release -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0.12
ARG SERVICE
ENV SERVICE_NAME=${SERVICE} ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet Vwp.${SERVICE_NAME}.Api.dll"]
