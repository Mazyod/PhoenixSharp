defmodule TesterWeb.Presence do
  use Phoenix.Presence,
    otp_app: :tester,
    pubsub_server: Tester.PubSub

  def fetch("tester:" <> _, presences) do
    for {key, %{metas: metas}} <- presences, into: %{} do
      {key, %{metas: metas, device: %{make: "Apple", model: "iPhone 11"}}}
    end
  end
end
