FROM mcr.microsoft.com/dotnet/sdk:5.0.201-focal-amd64 AS build-env
WORKDIR /app

COPY . ./
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/sdk:5.0.201-focal-amd64
WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "GcTesting.dll"]