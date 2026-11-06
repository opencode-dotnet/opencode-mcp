FROM ubuntu:22.04

ARG TARGETARCH
ARG SOURCE_REVISION

LABEL org.opencontainers.image.source="https://github.com/opencode-dotnet/opencode-mcp" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.description="OpenCode MCP Linux builder (Ubuntu 22.04, glibc 2.35)"

SHELL ["/bin/bash", "-e", "-o", "pipefail", "-c"]

# PowerShell's bundled runtime needs ICU even though the native product uses invariant globalization.
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      clang zlib1g-dev \
      ca-certificates curl git tar gzip unzip \
      libc6 libgcc-s1 libgssapi-krb5-2 libicu70 libssl3 libstdc++6 tzdata zlib1g \
 && rm -rf /var/lib/apt/lists/*

RUN case "$TARGETARCH" in \
      amd64) archive_arch=x64; sha256=1DA6D6D6035D651F20E70DDEE7C4C15AC6AB8B9FB40EE92240D91263F28A884B ;; \
      arm64) archive_arch=arm64; sha256=02EB9991AF447C7E7FE881ACDE195961AFD397C40B8EB1C2B3856B84EC2D85F2 ;; \
      *) echo "Unsupported builder architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && curl --fail --show-error --location \
      "https://github.com/PowerShell/PowerShell/releases/download/v7.6.9/powershell-7.6.9-linux-${archive_arch}.tar.gz" \
      --output /tmp/powershell.tar.gz \
 && echo "$sha256  /tmp/powershell.tar.gz" | sha256sum --check --strict - \
 && mkdir -p /opt/microsoft/powershell/7 \
 && tar -xzf /tmp/powershell.tar.gz -C /opt/microsoft/powershell/7 \
 && chmod +x /opt/microsoft/powershell/7/pwsh \
 && ln -s /opt/microsoft/powershell/7/pwsh /usr/bin/pwsh \
 && rm /tmp/powershell.tar.gz

# Capture before checking so a failed probe cannot be hidden by a pipeline's last command.
RUN architecture="$(uname -m)" \
 && printf '%s\n' "$architecture" \
 && case "$TARGETARCH:$architecture" in \
      amd64:x86_64|arm64:aarch64) ;; \
      *) echo "Builder architecture mismatch: $TARGETARCH:$architecture" >&2; exit 1 ;; \
    esac \
 && glibc="$(ldd --version)" \
 && printf '%s\n' "$glibc" \
 && [[ "${glibc%%$'\n'*}" == *" 2.35" ]] \
 && clang --version \
 && ld --version \
 && powershell="$(pwsh --version)" \
 && printf '%s\n' "$powershell" \
 && [[ "$powershell" == 'PowerShell 7.6.9' ]] \
 && git --version \
 && curl --version

WORKDIR /workspace
