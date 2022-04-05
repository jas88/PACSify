FROM mcr.microsoft.com/dotnet/runtime:5.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["PACSify/PACSify.csproj", "PACSify/"]
RUN dotnet restore "PACSify/PACSify.csproj"
COPY . .
WORKDIR "/src/PACSify"
RUN dotnet build "PACSify.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PACSify.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PACSify.dll"]
