# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restaurar primero sólo con los archivos de proyecto aprovecha la caché de capas.
COPY global.json nuget.config ./
COPY src/CreditDesk.Api/CreditDesk.Api.csproj src/CreditDesk.Api/
RUN dotnet restore src/CreditDesk.Api/CreditDesk.Api.csproj

COPY src/ src/
RUN dotnet publish src/CreditDesk.Api/CreditDesk.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Usuario sin privilegios incluido en las imágenes oficiales de .NET 8.
USER app
ENTRYPOINT ["dotnet", "CreditDesk.Api.dll"]
