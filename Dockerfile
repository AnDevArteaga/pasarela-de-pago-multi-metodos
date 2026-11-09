FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json* ./
RUN npm install
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Pasarela.sln ./
COPY src/Pasarela.Api/Pasarela.Api.csproj src/Pasarela.Api/
COPY tests/Pasarela.Tests/Pasarela.Tests.csproj tests/Pasarela.Tests/
RUN dotnet restore Pasarela.sln
COPY db ./db
COPY src ./src
COPY tests ./tests
RUN dotnet test Pasarela.sln -c Release --no-restore
COPY --from=web /src/web/dist ./src/Pasarela.Api/wwwroot
RUN dotnet publish src/Pasarela.Api/Pasarela.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Pasarela.Api.dll"]
