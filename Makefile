.PHONY: test build build-server build-agent package-server

test:
	go test ./...

build:
	go build ./...

build-server:
	go build -o bin/ip-multiviewer-server ./apps/server/cmd/server

build-agent:
	go build -o bin/ip-multiviewer-agent ./apps/agent/cmd/agent

package-server:
	pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/package-server.ps1
