FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

ENV BOT_TOKEN=""
ENV REDIS_URL="redis:6379"
ENV TZ="Europe/Moscow"

ENTRYPOINT ["dotnet", "Ipoteka.dll"]