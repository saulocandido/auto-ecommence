FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/AutoCommerce.StoreManagement/AutoCommerce.StoreManagement.csproj", "src/AutoCommerce.StoreManagement/"]
COPY ["src/AutoCommerce.Shared/AutoCommerce.Shared.csproj", "src/AutoCommerce.Shared/"]
RUN dotnet restore "src/AutoCommerce.StoreManagement/AutoCommerce.StoreManagement.csproj"
COPY . .
RUN dotnet build "src/AutoCommerce.StoreManagement/AutoCommerce.StoreManagement.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "src/AutoCommerce.StoreManagement/AutoCommerce.StoreManagement.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime image ships with Chromium + all its shared-library deps preinstalled,
# which Playwright needs to drive the Shopify admin flow.
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-jammy
WORKDIR /app
COPY --from=publish /app/publish .
# Playwright browsers live at /ms-playwright in this image; tell .NET about it.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Install Xvfb + x11vnc + noVNC for manual browser login via web viewer
RUN apt-get update && apt-get install -y --no-install-recommends \
    xvfb x11vnc novnc websockify \
    && rm -rf /var/lib/apt/lists/*

# Start Xvfb on :99 before the app
ENV DISPLAY=:99
# noVNC will be started on-demand by the app on port 6080
EXPOSE 8080 6080
ENTRYPOINT ["bash", "-c", "Xvfb :99 -screen 0 1920x1080x24 -nolisten tcp &>/dev/null & sleep 0.5 && exec dotnet AutoCommerce.StoreManagement.dll"]
