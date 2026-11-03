# syntax=docker/dockerfile:1
# Dormant's container image. Build from the repository root:
#
#   docker build -f deploy/docker/Dockerfile -t dormant .
#   docker run --rm -v dormant-state:/var/lib/dormant -v ./dormant.yaml:/etc/dormant/dormant.yaml:ro \
#       -e DORMANT_ERP_PROD='Server=...' dormant
#
# The image holds only the self-contained dormant executable on Ubuntu's chiseled runtime base: no shell, no package
# manager, running as the non-root user 'app' (1654). History and reports go to the volume /var/lib/dormant
# (decision D-060). The 'demo' target adds the demo tool used by deploy/demo.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403-noble AS build
ARG TARGETARCH
# Set by the release workflow from the tag; empty for local builds, which keep the version in Directory.Build.props.
ARG VERSION=
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in \
        amd64) rid=linux-x64 ;; \
        arm64) rid=linux-arm64 ;; \
        *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/Dormant.Cli -c Release -r "$rid" ${VERSION:+-p:Version=$VERSION} -o /out/dormant \
    && dotnet publish demo/Dormant.Demo -c Release -r "$rid" ${VERSION:+-p:Version=$VERSION} -o /out/demo \
    && mkdir -p /out/state/reports \
    && chmod -R 0700 /out/state

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra AS dormant
COPY --from=build /out/dormant/dormant /usr/local/bin/dormant
COPY --from=build --chown=1654:1654 /out/state /var/lib/dormant
# The single-file executable unpacks its native libraries here.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/dormant/.bundle
USER 1654
VOLUME ["/var/lib/dormant"]
ENTRYPOINT ["/usr/local/bin/dormant"]
CMD ["run", "-c", "/etc/dormant/dormant.yaml"]

FROM dormant AS demo
COPY --from=build /out/demo/dormant-demo /usr/local/bin/dormant-demo

# The default image is Dormant alone.
FROM dormant
