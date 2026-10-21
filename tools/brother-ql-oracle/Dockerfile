# Runs brother_ql (GPLv3) as a black box to produce reference jobs for BrotherRaster's QL encoder.
# Nothing from brother_ql is copied into BrotherRaster: only the jobs it writes are kept, as test data.
FROM python:3.11-slim-bookworm@sha256:a36c24f9cbdf4fd0f52d67f0823eeac19c2028c637cecc392d97f980d4fec56b

COPY requirements.lock /tmp/requirements.lock
RUN pip install --no-cache-dir -r /tmp/requirements.lock

WORKDIR /oracle
COPY oracle.py /oracle/
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONWARNINGS=ignore
ENTRYPOINT ["python", "/oracle/oracle.py"]
