FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Api/Api.csproj src/Api/
RUN dotnet restore src/Api/Api.csproj

COPY src/ src/
# The contract is generated and committed by a developer build, never inside the
# image: the CI check compares the committed file against a clean build, and a
# second generator here would be a second thing to keep true. GenerateContract is
# this project's own target - the package's hook is already off - so it is the
# switch that actually stops generation here.
RUN dotnet publish src/Api/Api.csproj -c Release -o /app \
    -p:GenerateContract=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# wget serves the compose healthcheck; the aspnet image has no curl.
RUN apt-get update \
    && apt-get install --no-install-recommends -y wget \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]
