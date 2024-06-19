
dotnet publish ./src/Kassini/Kassini.csproj -r linux-x64 -p:PublishSingleFile=true --self-contained true -o "$PSScriptRoot\.artifacts\linux-x64"
dotnet publish ./src/Kassini/Kassini.csproj -r win-x64 -p:PublishSingleFile=true --self-contained true -o "$PSScriptRoot\.artifacts\win-x64"