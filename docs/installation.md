# Installation Instructions

Please reference these docs: https://docs.btcpayserver.org/Docker/#full-installation-for-technical-users

<!-- For the official documentation, refer to [Full installation (for technical users) - docs.btcpayserver.org](https://docs.btcpayserver.org/Docker/#full-installation-for-technical-users) -->


```sh
mkdir BTCPayServer
cd BTCPayServer

# Clone the docker fragment repository
git clone https://github.com/btcpay-zcash/btcpayserver-docker
cd btcpayserver-docker
git checkout feat/zec

# Run btcpay-setup.sh with the right parameters
export BTCPAY_HOST="btcpay.example.com"
export NBITCOIN_NETWORK="mainnet"
export BTCPAYGEN_CRYPTO1="zec"
export BTCPAYGEN_ADDITIONAL_FRAGMENTS=""
export BTCPAYGEN_REVERSEPROXY="nginx"
. ./btcpay-setup.sh -i
```

## Full Node

Running a full node (with `zebra` and `lightwalletd`)

```sh
export BTCPAYGEN_EXCLUDE_FRAGMENTS="$BTCPAYGEN_EXCLUDE_FRAGMENTS;zcash"
export BTCPAYGEN_ADDITIONAL_FRAGMENTS="$BTCPAYGEN_ADDITIONAL_FRAGMENTS;zcash-fullnode"
. ./btcpay-setup.sh -i
```

## Existing BTCPayServer Instance

As root, run `. btcpay-setup.sh`; this will show you the environment variable it is expecting. For example, if you support `btc` and `ltc` already, and want to add `zec`:


**Example**

**`git checkout` into fork**

```sh
cd btcpayserver-docker
git checkout feat/zec
```

```sh
export BTCPAYGEN_CRYPTO3='zec'
. btcpay-setup.sh -i
```

## Lightwalletd Custom Docker Volume

Manually create a custom fragment:

**[`docker-compose-generator/docker-fragments/zcash-lightwalletd.custom.yml`](docs/zcash-lightwalletd.custom.yml)**

**Example Changes (will not work by itself)**

```yml
services:
  zebra:
    # ...
    volumes:
      - docker_zebrad-cache:/home/zebra/.cache/zebra
  lightwalletd:
    # ...
    volumes:
      - docker_lwd-cache:/var/lib/lightwalletd/db

volumes:
  # ...
  docker_zebrad-cache:
    external: true
  docker_lwd-cache:
    external: true

exclusive:
  - zcash-node
```

```sh
export BTCPAYGEN_EXCLUDE_FRAGMENTS="$BTCPAYGEN_EXCLUDE_FRAGMENTS;zcash;zcash-fullnode"
export BTCPAYGEN_ADDITIONAL_FRAGMENTS="$BTCPAYGEN_ADDITIONAL_FRAGMENTS;zcash-lightwalletd.custom"
. ./btcpay-setup.sh -i
```

## Setup Your (View-Only) Zcash Wallet

Use a mobile or desktop wallet such as YWallet or Zingo, that supports a Viewing Key export.


## Install and Setup Zcash Plugin

![Manage Plugins](./images/manage-plugins.jpg)

![Install Zcash Plugin](./images/install-zcash-plugin.jpg)

![Zcash Settings](./images/zcash-settings.jpg)

![Create Wallet](./images/create-wallet.jpg)

![Set Confirmations](./images/set-confirmations.jpg)
