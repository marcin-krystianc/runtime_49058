FROM mcr.microsoft.com/dotnet/sdk:5.0.201-focal-amd64 AS build-env
WORKDIR /app

COPY . ./
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/sdk:5.0.201-focal-amd64
COPY --from=build-env /app/out .
RUN apt update && apt install htop
WORKDIR /app
ENTRYPOINT ["dotnet", "GcTesting.dll"]