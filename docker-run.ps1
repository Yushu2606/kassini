param(
    # Path of the configuration file
    [Parameter(Mandatory=$true)]
    [string]$config,

    [Parameter(Mandatory=$true)]
    [string]$name
) 

docker run --name ${name} -d -p 8084:8084 -v "${config}:/etc/kassini/config.yml" kassini:latest
