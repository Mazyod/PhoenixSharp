defmodule Tester.Application do
  # See https://hexdocs.pm/elixir/Application.html
  # for more information on OTP Applications
  @moduledoc false

  use Application

  @impl true
  def start(_type, _args) do
    children = [
      TesterWeb.Telemetry,
      {DNSCluster, query: Application.get_env(:tester, :dns_cluster_query) || :ignore},
      {Phoenix.PubSub, name: Tester.PubSub},
      # Start to serve requests, typically the last entry
      TesterWeb.Endpoint,
      TesterWeb.Presence
    ]

    # See https://hexdocs.pm/elixir/Supervisor.html
    # for other strategies and supported options
    opts = [strategy: :one_for_one, name: Tester.Supervisor]
    Supervisor.start_link(children, opts)
  end

  # Tell Phoenix to update the endpoint configuration
  # whenever the application is updated.
  @impl true
  def config_change(changed, _new, removed) do
    TesterWeb.Endpoint.config_change(changed, removed)
    :ok
  end
end
