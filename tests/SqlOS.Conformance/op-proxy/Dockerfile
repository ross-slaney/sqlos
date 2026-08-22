# TLS front for the SqlOS example OP running on the host.
#
# The OIDCC *config* certification plan requires every advertised endpoint to
# be https (CheckDiscEndpointAllEndpointsAreHttps etc.), so the plain-http
# App X on the host is fronted by this nginx with a self-signed cert for the
# in-network hostname "sqlos-op". This mirrors the conformance suite's own
# docker-compose-localtest.yml, whose sample OP is an https compose service
# with a self-signed cert; the suite's HTTP clients and its HtmlUnit
# automation browser both tolerate it in devmode.
FROM nginx:1.27.3
RUN apt-get update \
	&& apt-get install -y openssl \
	&& openssl req -x509 -nodes -days 3650 -newkey rsa:2048 \
		-keyout /etc/ssl/private/op-selfsigned.key \
		-out /etc/ssl/certs/op-selfsigned.crt \
		-subj "/CN=sqlos-op" \
		-addext "subjectAltName=DNS:sqlos-op" \
	&& apt-get clean
COPY default.conf.template /etc/nginx/templates/default.conf.template
