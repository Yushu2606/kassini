FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /App

# Copy everything
COPY . ./

# Generate dev certificate
RUN dotnet dev-certs https

# Restore as distinct layers
RUN dotnet restore ./src/Kassini/Kassini.csproj
# Build and publish a release
RUN dotnet publish ./src/Kassini/Kassini.csproj -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0

COPY --from=build-env /root/.dotnet/corefx/cryptography/x509stores/my/* /root/.dotnet/corefx/cryptography/x509stores/my/
WORKDIR /App
COPY --from=build-env /App/out .

# The /etc/kassini/config.yml is bound to a host file
ENTRYPOINT ["dotnet", "yarp.dll", "/etc/kassini/config.yml"]
