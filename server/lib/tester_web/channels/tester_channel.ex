defmodule TesterWeb.TesterChannel do
  use TesterWeb, :channel
  require Logger
  alias TesterWeb.Presence

  @impl true
  def join("tester:" <> _id, params, socket) do
    with %{"auth" => auth} <- params do
      send(self(), :after_join)
      {:ok, assign(socket, :auth, auth)}
    else
      _ -> {:error, %{message: "auth required"}}
    end
  end

  @impl true
  def handle_in("reply_test", params, socket) do
    Logger.debug("[reply_test]: #{inspect(params)}")
    {:reply, {:ok, params}, socket}
  end

  @impl true
  def handle_in("push_test", params, socket) do
    Logger.debug("[push_test]: #{inspect(params)}")
    push(socket, "push_test", %{"works" => true})
    {:noreply, socket}
  end

  @impl true
  def handle_in("error_test", _, socket) do
    Logger.debug("[error_test]")
    {:reply, :error, socket}
  end

  @impl true
  def handle_in("timeout_test", _, socket) do
    Logger.debug("[timeout_test]")
    {:noreply, socket}
  end

  @impl true
  def handle_info(:after_join, socket) do
    push(socket, "after_join", %{message: "Welcome!"})

    {:ok, _} =
      Presence.track(socket, socket.assigns.auth, %{
        online_at: inspect(System.system_time(:second))
      })

    push(socket, "presence_state", Presence.list(socket))
    {:noreply, socket}
  end
end
