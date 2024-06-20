param(
    [Parameter(Mandatory=$true)]
    [string]$name
)

docker stop ${name}
docker rm ${name}
