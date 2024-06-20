# YARP.exe

Usage:

```console
# Builds the `kassini/yarp:latest` Docker image locally
./docker-build.ps1

# Starts a new container named `myproxy` with the local configuration file `.\src\Kassini\simple.yml`
.\docker-run.ps1 -name myproxy -config .\src\Kassini\simple.yml

# Stops the container named `myproxy`
```