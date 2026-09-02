# Build from the repository root. Override tags with reviewed immutable digests for deployment.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.101
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
FROM ${SDK_IMAGE} AS build
WORKDIR /source
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json NuGet.Config README.md LICENSE ./
COPY docs/ docs/
COPY src/ src/
COPY samples/Skopka.Chat.BotGateway/ samples/Skopka.Chat.BotGateway/
RUN dotnet restore samples/Skopka.Chat.BotGateway/Skopka.Chat.BotGateway.csproj --configfile NuGet.Config
RUN dotnet publish samples/Skopka.Chat.BotGateway/Skopka.Chat.BotGateway.csproj -c Release --no-restore -o /app

FROM build AS checks
COPY tests/Skopka.Chat.Bots.Tests/ tests/Skopka.Chat.Bots.Tests/
COPY tests/Skopka.Chat.FuzzTests/ tests/Skopka.Chat.FuzzTests/
RUN dotnet restore tests/Skopka.Chat.Bots.Tests/Skopka.Chat.Bots.Tests.csproj --configfile NuGet.Config
RUN dotnet test --project tests/Skopka.Chat.Bots.Tests --configuration Release --no-restore
RUN dotnet restore tests/Skopka.Chat.FuzzTests/Skopka.Chat.FuzzTests.csproj --configfile NuGet.Config
RUN dotnet run --project tests/Skopka.Chat.FuzzTests --configuration Release -- --replay tests/Skopka.Chat.FuzzTests/corpus

FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Skopka.Chat.BotGateway.dll"]
