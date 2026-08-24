# ClassGraph Backend für Render

Dieser Ordner enthält nur den öffentlich deploybaren .NET-Backendteil von ClassGraph.
Render baut ihn über das `Dockerfile`; `render.yaml` beschreibt einen kostenlosen
Web Service mit `/health` als Bereitschaftsprüfung.

Der WebSocket-Endpunkt `/ws` akzeptiert im Onlinebetrieb ausschließlich Anfragen,
die den geheimen Header `X-ClassGraph-Proxy-Key` enthalten. Derselbe geheime Wert
muss in Render als `ClassGraph__ProxyKey` und in Sites als
`CLASSGRAPH_PROXY_KEY` gespeichert werden. Der Schlüssel gehört niemals in Git.
