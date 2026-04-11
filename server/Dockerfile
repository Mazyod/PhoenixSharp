# Build stage
FROM elixir:1.19-slim AS build

RUN apt-get update -y && \
    apt-get install -y --no-install-recommends ca-certificates && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

ENV MIX_ENV=prod

RUN mix local.hex --force && \
    mix local.rebar --force

WORKDIR /app

COPY mix.exs mix.lock ./
RUN mix deps.get --only $MIX_ENV && \
    mix deps.compile

COPY config config
COPY lib lib
COPY priv priv

RUN mix compile && mix release

# Runtime stage
FROM debian:trixie-slim

RUN apt-get update -y && \
    apt-get install -y --no-install-recommends libstdc++6 openssl libncurses6 locales && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
    sed -i '/en_US.UTF-8/s/^# //g' /etc/locale.gen && \
    locale-gen

ENV LANG=en_US.UTF-8
ENV LANGUAGE=en_US:en
ENV LC_ALL=en_US.UTF-8

WORKDIR /app

COPY --from=build /app/_build/prod/rel/tester ./

ENV PHX_SERVER=true

EXPOSE 4000

CMD ["bin/tester", "start"]
