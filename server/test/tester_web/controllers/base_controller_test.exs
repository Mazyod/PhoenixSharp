defmodule TesterWeb.BaseControllerTest do
  use TesterWeb.ConnCase

  setup %{conn: conn} do
    {:ok, conn: put_req_header(conn, "accept", "application/json")}
  end

  describe "basic" do
    test "access health-check endpoint", %{conn: conn} do
      conn = get(conn, ~p"/api/health-check")
      assert json_response(conn, 200) == %{"ok" => true}
    end
  end
end
