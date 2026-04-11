defmodule Tester.TesterChannelTest do
  use TesterWeb.ChannelCase
  alias TesterWeb.UserSocket

  setup do
    {:ok, _, socket} =
      UserSocket
      |> socket("user_id", %{some: :assign})
      |> subscribe_and_join(TesterWeb.TesterChannel, "tester:user_id", %{"auth" => "test"})

    {:ok, socket: socket}
  end

  test "it requires auth payload" do
    result =
      UserSocket
      |> socket("user_id", %{some: :assign})
      |> subscribe_and_join(TesterWeb.TesterChannel, "tester:user_id")

    assert {:error, _} = result
  end

  test "it receives a welcome message & presence state after joining", %{socket: socket} do
    assert_push("after_join", %{message: "Welcome!"})
    presence_list = TesterWeb.Presence.list(socket)
    assert_push("presence_state", ^presence_list)
  end
end
