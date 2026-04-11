defmodule TesterWeb.Router do
  use TesterWeb, :router

  pipeline :api do
    plug :accepts, ["json"]
  end

  scope "/api", TesterWeb do
    pipe_through :api

    get "/health-check", BaseController, :health_check
  end
end
