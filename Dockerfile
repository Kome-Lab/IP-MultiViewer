FROM golang:1.26-trixie AS build
WORKDIR /src
COPY go.mod ./
COPY . .
RUN go build -o /out/ip-multiviewer-server ./apps/server/cmd/server
FROM gcr.io/distroless/base-debian13
COPY --from=build /out/ip-multiviewer-server /usr/local/bin/ip-multiviewer-server
USER nonroot:nonroot
ENTRYPOINT ["/usr/local/bin/ip-multiviewer-server"]
