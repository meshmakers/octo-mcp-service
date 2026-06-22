FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8443

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE
ARG OCTO_VERSION
ARG OCTO_CORE_LIB_VERSION
ARG OCTO_COMM_VERSION

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /src
COPY . .
ENV NUGET_XMLDOC_MODE=none
WORKDIR "/src/src/McpServices"
# `${OCTO_VERSION:+-p:OctoVersion=…}` only emits the -p switch when
# OCTO_VERSION is non-empty. Passing `-p:OctoVersion=` (blank) suppresses
# the Directory.Build.props fallback (3.3.* / 0.1.*) and NU1015 fires.
RUN dotnet build "./McpServices.csproj" -o /app/build -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} ${OCTO_VERSION:+-p:OctoVersion=${OCTO_VERSION} -p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION} -p:OctoCommVersion=${OCTO_COMM_VERSION}}

FROM build AS publish
RUN dotnet publish "./McpServices.csproj" -o /app/publish /p:UseAppHost=false -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} ${OCTO_VERSION:+-p:OctoVersion=${OCTO_VERSION} -p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION} -p:OctoCommVersion=${OCTO_COMM_VERSION}}

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Backend.McpServices.dll"]
