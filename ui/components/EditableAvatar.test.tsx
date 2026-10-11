import { StrictMode } from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { NextIntlClientProvider } from "next-intl";
import { describe, expect, it, vi } from "vitest";
import * as avatarImage from "@/lib/avatarImage";
import { EditableAvatar } from "./EditableAvatar";

const useAuth = vi.fn();
const uploadAvatar = vi.fn();
const clearAvatar = vi.fn();
const setAvatarUrl = vi.fn();

vi.mock("@/components/AuthProvider", () => ({
  useAuth: () => useAuth(),
}));

vi.mock("@/lib/api", () => ({
  ApiError: class ApiError extends Error {
    status: number;
    constructor(status: number, message: string) {
      super(message);
      this.status = status;
    }
  },
  uploadAvatar: (...args: unknown[]) => uploadAvatar(...args),
  clearAvatar: (...args: unknown[]) => clearAvatar(...args),
}));

const messages = {
  AvatarPicker: {
    changeAvatar: "Change profile picture",
    dialogTitle: "Profile picture",
    dialogDescription: "Upload a photo or keep your initials.",
    uploadPhoto: "Upload photo",
    useInitials: "Use initials",
    cropTitle: "Crop your photo",
    cropHint: "Drag to reposition. Use the slider to zoom.",
    zoom: "Zoom",
    save: "Save",
    back: "Back",
    cancel: "Cancel",
    fileTooLarge: "That file is too large. Choose one under 256 KB.",
    invalidFile: "That file could not be read. Try a JPEG, PNG, or WebP image.",
    genericError: "Something went wrong. Please try again.",
  },
};

function renderAvatar(avatarUrl: string | null = null) {
  return render(
    <StrictMode>
      <NextIntlClientProvider locale="en" messages={messages}>
        <EditableAvatar label="MK" avatarUrl={avatarUrl} />
      </NextIntlClientProvider>
    </StrictMode>,
  );
}

describe("EditableAvatar", () => {
  it("renders a plain avatar when signed out", () => {
    useAuth.mockReturnValue({ token: null, setAvatarUrl });
    renderAvatar();
    expect(
      screen.queryByRole("button", { name: "Change profile picture" }),
    ).toBeNull();
    expect(screen.getByText("MK")).toBeTruthy();
  });

  it("opens the upload menu when signed in", () => {
    useAuth.mockReturnValue({ token: "tok", setAvatarUrl });
    renderAvatar();

    const button = screen.getByRole("button", { name: "Change profile picture" });
    const pencil = button.querySelector("svg")?.parentElement;
    expect(pencil?.className).toContain("opacity-0");
    expect(pencil?.className).toContain("inset-0");
    expect(pencil?.className).toContain("group-hover:opacity-100");

    fireEvent.click(button);
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Upload photo" })).toBeTruthy();
  });

  it("uploads the cropped photo when signed in", async () => {
    useAuth.mockReturnValue({ token: "tok", setAvatarUrl });
    uploadAvatar.mockResolvedValue({
      avatarUrl: "/api/users/00000000-0000-4000-8000-000000000001/avatar?v=2",
    });
    const blob = new Blob(["jpeg"], { type: "image/jpeg" });
    vi.spyOn(avatarImage, "exportCroppedAvatar").mockResolvedValue(blob);
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:avatar-preview");
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    vi.stubGlobal(
      "Image",
      class FakeImage {
        onload: (() => void) | null = null;
        onerror: (() => void) | null = null;
        naturalWidth = 400;
        naturalHeight = 300;
        #src = "";

        set src(value: string) {
          this.#src = value;
          this.onload?.();
        }

        get src() {
          return this.#src;
        }
      },
    );

    try {
      const { container } = renderAvatar();
      fireEvent.click(
        screen.getByRole("button", { name: "Change profile picture" }),
      );
      fireEvent.click(screen.getByRole("button", { name: "Upload photo" }));
      const input = container.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      fireEvent.change(input, {
        target: {
          files: [new File(["photo"], "photo.png", { type: "image/png" })],
        },
      });

      await waitFor(() => {
        expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
      });
      fireEvent.click(screen.getByRole("button", { name: "Save" }));

      await waitFor(() => {
        expect(uploadAvatar).toHaveBeenCalledWith(blob);
        expect(setAvatarUrl).toHaveBeenCalledWith(
          "/api/users/00000000-0000-4000-8000-000000000001/avatar?v=2",
        );
      });
    } finally {
      vi.unstubAllGlobals();
      vi.restoreAllMocks();
    }
  });

  it("shows the chosen photo and keeps it covering the crop square", async () => {
    useAuth.mockReturnValue({ token: "tok", setAvatarUrl });
    const revoked: string[] = [];
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:avatar-preview");
    vi.spyOn(URL, "revokeObjectURL").mockImplementation((url: string) => {
      revoked.push(String(url));
    });
    vi.stubGlobal(
      "Image",
      class FakeImage {
        onload: (() => void) | null = null;
        onerror: (() => void) | null = null;
        naturalWidth = 400;
        naturalHeight = 300;
        #src = "";

        set src(value: string) {
          this.#src = value;
          this.onload?.();
        }

        get src() {
          return this.#src;
        }
      },
    );

    try {
      const { container } = renderAvatar();
      fireEvent.click(
        screen.getByRole("button", { name: "Change profile picture" }),
      );
      fireEvent.click(screen.getByRole("button", { name: "Upload photo" }));

      const input = container.querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      fireEvent.change(input, {
        target: {
          files: [new File(["photo"], "photo.png", { type: "image/png" })],
        },
      });

      await waitFor(() => {
        expect(container.querySelector("img")?.getAttribute("src")).toBe(
          "blob:avatar-preview",
        );
      });
      expect(revoked).toEqual([]);

      const frame = container.querySelector("img")?.parentElement;
      expect(frame).toBeTruthy();
      fireEvent.pointerDown(frame!, { clientX: 0, clientY: 0, pointerId: 1 });
      fireEvent.pointerMove(frame!, { clientX: 900, clientY: 900, pointerId: 1 });

      const dragged = container.querySelector("img")!;
      const height = parseFloat(dragged.style.height);
      const width = parseFloat(dragged.style.width);
      expect(parseFloat(dragged.style.top)).toBe(0);
      expect(parseFloat(dragged.style.left)).toBe(0);
      expect(width).toBeGreaterThan(height);

      fireEvent.pointerMove(frame!, { clientX: -900, clientY: -900, pointerId: 1 });
      const pulled = container.querySelector("img")!;
      expect(parseFloat(pulled.style.top)).toBe(0);
      expect(parseFloat(pulled.style.left)).toBe(
        parseFloat(pulled.style.height) - parseFloat(pulled.style.width),
      );

      fireEvent.click(screen.getByRole("button", { name: "Back" }));
      await waitFor(() => {
        expect(revoked).toEqual(["blob:avatar-preview"]);
      });
    } finally {
      vi.unstubAllGlobals();
      vi.restoreAllMocks();
    }
  });

  it("clears a photo back to initials", async () => {
    useAuth.mockReturnValue({ token: "tok", setAvatarUrl });
    clearAvatar.mockResolvedValue({ avatarUrl: null });
    renderAvatar("/api/users/00000000-0000-4000-8000-000000000001/avatar?v=1");

    fireEvent.click(
      screen.getByRole("button", { name: "Change profile picture" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Use initials" }));

    await waitFor(() => {
      expect(clearAvatar).toHaveBeenCalled();
      expect(setAvatarUrl).toHaveBeenCalledWith(null);
    });
  });
});
