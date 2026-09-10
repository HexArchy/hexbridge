FROM golang:1.25-alpine AS build
WORKDIR /src
COPY go.mod ./
COPY main.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/hexbridge-relay .

FROM scratch
COPY --from=build /out/hexbridge-relay /hexbridge-relay
EXPOSE 47702/udp
ENTRYPOINT ["/hexbridge-relay"]
CMD ["-listen", ":47702"]
