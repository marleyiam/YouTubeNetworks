
FROM mcr.microsoft.com/dotnet/core/sdk:3.0 AS build-env
WORKDIR /app

# copy everything else and build
COPY SysExtensions SysExtensions
COPY YtCli YtCli
COPY YtReader YtReader
COPY "Mutuo.Etl" "Mutuo.Etl"
WORKDIR /app/YtCli
#RUN dotnet restore
RUN dotnet publish -c Release -o publish

# build runtime image
FROM mcr.microsoft.com/dotnet/core/runtime:3.0-alpine
WORKDIR /app
COPY --from=build-env /app/YtCli/publish  ./
#ENTRYPOINT ["dotnet", "ytnetworks.dll"] 