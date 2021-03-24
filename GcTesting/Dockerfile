FROM mcr.microsoft.com/dotnet/sdk:5.0.201-focal-amd64 AS build-env
WORKDIR /app

COPY . ./
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/sdk:5.0.201-focal-amd64
WORKDIR /app
COPY --from=build-env /app/out .
RUN apt update && apt install htop
ENV PATH="/root/.dotnet/tools:${PATH}"
RUN dotnet tool install --global dotnet-counters
RUN dotnet tool install --global dotnet-dump
ENTRYPOINT ["dotnet", "GcTesting.dll"]
