$RuntimeIdentifiers = @(
    "win-x64",
    "win-arm64",
    "linux-x64",
    "linux-arm64",
    "osx-x64",
    "osx-arm64"
)

foreach ($RuntimeIdentifier in $RuntimeIdentifiers) {
    dotnet publish ./src/Kassini/Kassini.csproj `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output "$PSScriptRoot\.artifacts\$RuntimeIdentifier"
}
