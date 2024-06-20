# YARP.exe

Usage:

```console
# Builds the `kassini/yarp:latest` Docker image locally
./docker-build.ps1

# Starts a new container named `myproxy` with the local configuration file `.\src\Kassini\simple.yml`
.\docker-run.ps1 -name myproxy -config .\src\Kassini\simple.yml

# Stops the container named `myproxy`
```

From Aspire:

```c#
var kassini = new ContainerResource("kassini");

builder.AddResource(kassini)
    .WithImage("kassini/yarp", "latest")
    .WithBindMount("D:\\kassini\\src\\Kassini\\simple.yml", "/etc/kassini/config.yml")
    .WithHttpEndpoint(targetPort: 8084, name: "http") // Or any port that the kassini configuration exposes
    .WithOtlpExporter()
    ;
```