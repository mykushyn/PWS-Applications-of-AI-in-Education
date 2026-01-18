AI PWS

.NET start:
dotnet run --project WebApplication0.0/WebApplication0.0.csproj

PROJECT UPLOAD TO PLESK:
dotnet publish -c Release -f net8.0 -r linux-x64 --self-contained false -o ./publish

^^ Publish file downloaden als .zip, alles in httpdocs op plesk vervang je met alles in /publish
