defmodule TesterWeb.FallbackController do
  use TesterWeb, :controller

  def call(conn, {:error, :not_found}) do
    conn
    |> put_status(:not_found)
    |> put_view(json: TesterWeb.ErrorJSON)
    |> render(:"404")
  end
end
