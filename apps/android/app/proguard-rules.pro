# The app keeps nothing by reflection: Compose, coroutines and Kotlin publish their own consumer rules,
# and the protocol's XML reader uses the platform's parser. R8's defaults are enough.
