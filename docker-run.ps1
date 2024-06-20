param(
    # Path of the configuration file
    [Parameter(Mandatory=$true)]
    [string]$config,

    [Parameter(Mandatory=$true)]
    [string]$name
) 

docker run --name ${name} -d --network=host -v "${config}:/App/cfg/config.yml" kassini/yarp:latest
