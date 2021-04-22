FROM mcr.microsoft.com/dotnet/sdk:6.0.100-preview.3-focal-amd64 AS build-env
WORKDIR /app

COPY . ./
RUN ls -al ./
RUN dotnet publish -c Release -o out --self-contained true --runtime linux-x64 

#FROM ubuntu:focal
FROM mcr.microsoft.com/dotnet/sdk:6.0.100-preview.3-focal-amd64

WORKDIR /app
COPY --from=build-env /app/out .
COPY --from=build-env /app/Linux.x64.Release .
RUN apt update && apt install htop
ENV PATH="/root/.dotnet/tools:${PATH}"
# RUN dotnet tool install --global dotnet-counters
# RUN dotnet tool install --global dotnet-dump
ENTRYPOINT ["./GcTesting"]

