// `@sqlos/headless` is a `file:` dependency, so npm links it to
// ../../packages/headless. Metro follows the symlink to its real path and would
// otherwise resolve the package's peer dependencies (react, react-native) from
// packages/headless/node_modules — a second React copy when the package's
// devDependencies are installed, which breaks hooks ("Invalid hook call").
// Force those peers to this app's copies and watch the linked package.
const path = require("node:path");
const { getDefaultConfig } = require("expo/metro-config");

const projectRoot = __dirname;
const headlessRoot = path.resolve(projectRoot, "../../packages/headless");

const config = getDefaultConfig(projectRoot);

config.watchFolders = [...(config.watchFolders ?? []), headlessRoot];

const appOwnedPackages = new Set(["react", "react-native"]);

function packageNameOf(moduleName) {
  const parts = moduleName.split("/");
  return moduleName.startsWith("@") ? parts.slice(0, 2).join("/") : parts[0];
}

const defaultResolveRequest = config.resolver.resolveRequest;
config.resolver.resolveRequest = (context, moduleName, platform) => {
  const resolve = defaultResolveRequest ?? context.resolveRequest;
  if (
    appOwnedPackages.has(packageNameOf(moduleName)) &&
    context.originModulePath.startsWith(headlessRoot)
  ) {
    return resolve(
      { ...context, originModulePath: path.join(projectRoot, "index.js") },
      moduleName,
      platform,
    );
  }
  return resolve(context, moduleName, platform);
};

module.exports = config;
