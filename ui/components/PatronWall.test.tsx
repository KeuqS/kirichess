import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { PatronWall } from "./PatronWall";
import { PATRON_DEMOS } from "@/lib/patronDemos";

describe("PatronWall", () => {
  it("renders every patron name", () => {
    render(
      <PatronWall
        patrons={PATRON_DEMOS}
        heading="Patrons"
        intro="Thanks."
        listLabel="Patron supporters"
      />,
    );

    expect(screen.getByRole("list", { name: "Patron supporters" })).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(50);
    expect(screen.getByText("Mira")).toBeInTheDocument();
    expect(screen.getByText("Wout")).toBeInTheDocument();
  });
});
