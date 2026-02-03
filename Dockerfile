FROM rust:1.85 AS rust-build
WORKDIR /src
COPY native/lean-crypto-ffi /src/native/lean-crypto-ffi
RUN cargo build --release --manifest-path /src/native/lean-crypto-ffi/Cargo.toml

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN mkdir -p /src/src/Lean.Client/runtimes/linux-x64/native \
    && cp /src/native/lean-crypto-ffi/target/release/liblean_crypto_ffi.so /src/src/Lean.Client/runtimes/linux-x64/native/
ARG GIT_SHA=unknown
ENV LEAN_GIT_SHA=$GIT_SHA
RUN dotnet publish src/Lean.Client/Lean.Client.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["./Lean.Client"]
