import { useEffect, useState } from "react";
import { api, type Brand, type Me } from "./api";
import { AppShell } from "./AppShell";
import { SignIn } from "./SignIn";
import { Setup, type SetupStatus } from "./Setup";

export function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined);
  const [brand, setBrand] = useState<Brand | null>(null);
  const loadBrand = () => api.get<Brand>("/brand").then((b) => { setBrand(b); document.title = b.productName; }).catch(() => undefined);
  const [setup, setSetup] = useState<SetupStatus | null>(null);

  useEffect(() => {
    void loadBrand();
    api.get<SetupStatus>("/setup/status").then(setSetup).catch(() => setSetup({ required: false, codeRequired: false }));
    api.get<Me>("/me").then(setMe).catch(() => setMe(null));
  }, []);

  if (me === undefined || !brand || !setup) return null;
  if (me === null && setup.required)
    return <Setup brand={brand} codeRequired={setup.codeRequired} onDone={(m) => { setSetup({ required: false, codeRequired: false }); setMe(m); }} />;
  if (me === null) return <SignIn brand={brand} onSignedIn={setMe} />;
  return <AppShell me={me} brand={brand} onBrandChanged={loadBrand} onSignedOut={() => setMe(null)} />;
}
