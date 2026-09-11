# The relay, as a container.
#
# Published by CI to ghcr.io/hexarchy/hexbridge-relay on every tag and on main,
# for amd64 and arm64. A server pulls it rather than building it — see
# docs/VPS.md.

FROM golang:1.25-alpine AS build
WORKDIR /src

COPY go.mod ./
# Every source file, not a list of them. The list was written when there was one
# file, and the image quietly stopped building the moment there were four.
COPY *.go ./

RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/hexbridge-relay .

FROM scratch
COPY --from=build /out/hexbridge-relay /hexbridge-relay

# Audio and input.
EXPOSE 47702/udp
# The pairing rendezvous, so two machines that cannot accept a connection can
# still be introduced.
EXPOSE 47703/tcp

ENTRYPOINT ["/hexbridge-relay"]
CMD ["-listen", ":47702", "-pair-listen", ":47703"]
