# Regenerates tests/BrotherRaster.Tests/Fixtures/brother-ql.
FIXTURES := ../../tests/BrotherRaster.Tests/Fixtures/brother-ql
OUT := $(CURDIR)/out

.PHONY: fixtures image clean
fixtures: image
	rm -rf $(OUT) && mkdir -p $(OUT)
	docker run --rm -u $$(id -u):$$(id -g) -v $(OUT):/out brother-raster-oracle /out
	rm -rf $(FIXTURES) && cp -r $(OUT) $(FIXTURES)

image:
	docker build -t brother-raster-oracle .

clean:
	rm -rf $(OUT)
