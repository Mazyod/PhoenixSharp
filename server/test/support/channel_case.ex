defmodule TesterWeb.ChannelCase do
  use ExUnit.CaseTemplate

  using do
    quote do
      @endpoint TesterWeb.Endpoint

      import Phoenix.ChannelTest
      import TesterWeb.ChannelCase
    end
  end
end
