import { useEffect, useState } from "react";
import { api, type Brand, type Me } from "./api";
import { AppShell } from "./AppShell";
import { SignIn } from "./SignIn";
import { Setup, type SetupStatus } from "./Setup";
import { session } from "./kit/host";
import { loadPluginsOnce } from "./kit/loader";
import { passwordLinkAt, SetPassword } from "./SetPassword";

export function App() {
  const [me, setMe] = useState<Me | null | undefined>(undefined);
  const [brand, setBrand] = useState<Brand | null>(null);
  const loadBrand = () => api.get<Brand>("/brand").then((b) => { setBrand(b); document.title = b.productName; }).catch(() => undefined);
  const [setup, setSetup] = useState<SetupStatus | null>(null);
  // Someone not signed in reads the spaces open to Anyone, when there are any, until they choose Sign in.
  const [openSpaces, setOpenSpaces] = useState<number | null>(null);
  const [signingIn, setSigningIn] = useState(false);
  // A password link opens its own screen, whoever is or is not signed in in this browser.
  const [link, setLink] = useState(() => passwordLinkAt(location.pathname, location.hash));

  useEffect(() => {
    void loadBrand();
    api.get<SetupStatus>("/setup/status").then(setSetup).catch(() => setSetup({ required: false, codeRequired: false }));
    api.get<Me>("/me").then(setMe).catch(() => {
      setMe(null);
      void api.get<unknown[]>("/spaces").then((s) => setOpenSpaces(s.length)).catch(() => setOpenSpaces(0));
    });
  }, []);

  // Once someone is signed in, the scripts of the plugins are loaded. What they add appears as it is registered.
  // The scripts stay for the life of the page, so they are told who is signed in now, at every sign-in and sign-out.
  useEffect(() => { session.me = me ?? null; if (me) void loadPluginsOnce(me); }, [me]);

  if (me === undefined || !brand || !setup) return null;
  if (link) return <SetPassword brand={brand} userId={link.userId} token={link.token} onDone={() => { history.replaceState(history.state, "", "/"); setLink(null); }} />;
  if (me === null && setup.required)
    return <Setup brand={brand} codeRequired={setup.codeRequired} onDone={(m) => { setSetup({ required: false, codeRequired: false }); setMe(m); }} />;
  if (me === null && openSpaces === null) return null;
  if (me === null && (signingIn || !openSpaces)) return <SignIn brand={brand} onSignedIn={(m) => { setSigningIn(false); setMe(m); }} />;
  return <AppShell key={me?.id ?? "guest"} me={me} brand={brand} onBrandChanged={loadBrand} onSignedOut={() => { setOpenSpaces(null); setMe(null); void api.get<unknown[]>("/spaces").then((s) => setOpenSpaces(s.length)).catch(() => setOpenSpaces(0)); }}
    onSignIn={() => setSigningIn(true)} />;
}
