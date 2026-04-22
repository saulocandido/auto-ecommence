FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/AutoCommerce.Configuration/AutoCommerce.Configuration.csproj", "src/AutoCommerce.Configuration/"]
COPY ["src/AutoCommerce.Shared/AutoCommerce.Shared.csproj", "src/AutoCommerce.Shared/"]
RUN dotnet restore "src/AutoCommerce.Configuration/AutoCommerce.Configuration.csproj"
COPY . .
RUN dotnet publish "src/AutoCommerce.Configuration/AutoCommerce.Configuration.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "AutoCommerce.Configuration.dll"]
