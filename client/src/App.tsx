import { useEffect, useState } from "react";
import { api, ApiError, type Brand, type Me } from "./api";
import { AppShell } from "./AppShell";
import { SignIn } from "./SignIn";

export function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined);
  const [brand, setBrand] = useState<Brand | null>(null);

  useEffect(() => {
    api.get<Brand>("/brand").then((b) => {
      setBrand(b);
      document.title = b.productName;
    }).catch(() => undefined);
    api.get<Me>("/me").then(setMe).catch((e) => setMe(e instanceof ApiError && e.status === 401 ? null : null));
  }, []);

  if (me === undefined || !brand) return null;
  if (me === null) return <SignIn brand={brand} onSignedIn={setMe} />;
  return <AppShell me={me} brand={brand} onSignedOut={() => setMe(null)} />;
}
