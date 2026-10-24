FROM golang:1.27-alpine AS build
WORKDIR /src

# Dependências numa camada separada, que fica em cache até o go.mod/go.sum mudar.
COPY go.mod go.sum ./
RUN go mod download

COPY . .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/processor ./cmd/processor

# Imagem final sem shell nem gerenciador de pacotes, rodando como usuário não-root.
FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/processor /processor
EXPOSE 8081
ENTRYPOINT ["/processor"]
