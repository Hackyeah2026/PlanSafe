# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["CrowdSim/CrowdSim.csproj", "CrowdSim/"]
RUN dotnet restore "CrowdSim/CrowdSim.csproj"

COPY . .
WORKDIR "/src/CrowdSim"
RUN dotnet publish "CrowdSim.csproj" -c Release -o /app/publish

# Runtime Stage (Nginx)
FROM nginx:alpine AS final
WORKDIR /usr/share/nginx/html

COPY --from=build /app/publish/wwwroot .
COPY nginx.conf /etc/nginx/nginx.conf

EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
