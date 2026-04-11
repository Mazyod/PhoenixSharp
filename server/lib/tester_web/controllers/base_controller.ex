defmodule TesterWeb.BaseController do
  use TesterWeb, :controller

  action_fallback(TesterWeb.FallbackController)

  def health_check(conn, _) do
    json(conn, %{ok: true})
  end
end
